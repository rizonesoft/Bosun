---
schema_version: 1
id: private-overlay
domain: 00-workspace
status: active
title: "TODO-05 -- Public core and private overlay"
---

# TODO-05 -- Public core and private overlay

> **Goal:** Bosun is developed in the public `rizonesoft/Bosun` repository, and the owner's machine and account specifics live only in the private overlay `rizonetech/Bosun-private`, cloned into the ignored `.private/` folder of the checkout. A stranger's clone builds and passes every gate without the overlay. The owner works in one folder, one command shows and syncs both repositories, and a push that would publish a secret or a private string is refused before it leaves the machine.

> **Current state:** Measured 2026-11-20. The repository went public with its full history and moved to `rizonesoft/Bosun` on 2026-11-20; GitHub secret scanning, push protection and private vulnerability reporting are on, with 0 open alerts. The empty private repository `rizonetech/Bosun-private` exists. Nothing reads an overlay yet: `.gitignore` does not ignore `.private/`, `scripts/setup-clone.ps1` only sets `core.hooksPath`, `scripts/hooks/` holds only `pre-commit`, and there is no `scripts/sync.ps1`. Owner specifics still in tracked files: the default `op://` item in `scripts/store-claude-token.ps1` (also named in `tests/start_run_test.ps1`), 1Password vault wording in `AGENTS.md`, `CHANGELOG.md` and `scripts/start-run.ps1`, owner-only rules mixed into `AGENTS.md`, machine paths in 26 tracked files outside `docs/reviews/` (mostly docs and comments), and a personal name in `tests/Bosun.Panel.Tests/Controls/QueueVisualTests.cs`. `gitleaks` is not installed and CI does not scan history.

## Inputs

- The approved design: `docs/specs/2026-11-20-public-core-private-overlay-design.md` (owner decisions of 2026-11-20: one overlay per repository, nested clone in `.private/`, plans and receipts stay public and scrubbed, no fresh history).
- `AGENTS.md` rules this file must keep: Bosun stands alone, never names a machine's folders in code, resolves a tool by explicit config, then discovery, then a setup-required state.
- `scripts/setup-clone.ps1`, `scripts/hooks/pre-commit`, `scripts/store-claude-token.ps1`, `scripts/start-run.ps1`, `.github/workflows/build.yml`, `.gitignore`, `CLAUDE.md`, `AGENTS.md`.
- -> XREF: D99 T01 §4 -- this file does the scrub and leak scan that section's open checks name; that section records the publish decision

## Outcome

- `.private/` is ignored, and every owner value the code needs resolves from a `BOSUN_*` variable, then `.private/config.toml`, then a setup-required message, proven by tests with and without the folder.
- `setup-clone.ps1` clones the overlay when the account can see it, a `post-merge` hook keeps it current, and `scripts/sync.ps1` reports and syncs both repositories in one line each.
- A `pre-push` hook refuses outgoing commits that contain a `gitleaks` finding or any line of `.private/deny-list.txt`, and CI scans the full public history.
- Agent rules load `.private/AGENTS.md` when present, and the public files carry no owner-only rule, vault item, machine path or personal name outside stamped history.

**Adjacency:** all=not-applicable (repository tooling for the owner's checkout: no product records, pages or user data)

**Adjacency rationale:** The overlay is a development-workflow concern, not a product feature: the panel, `bosun.exe` and the loggers never read it. Reverse paths are still owned: deleting `.private/` and unsetting the hooks returns a plain public checkout, and each script that changes git config prints its undo.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Overlay folder, config lookup and the rule that admits it | -- | [x] |
| 2 | §2 | Overlay clone in setup and pull after merge | §1 | [x] |
| 3 | §3 | One sync command for both repositories | §2 | [x] |
| 4 | §4 | Pre-push leak guard with gitleaks and the private deny list | §1 | [ ] |
| 5 | §5 | Agent rules split into public and overlay halves | §1 | [ ] |
| 6 | §6 | Full-history secret scan in CI | §4 | [ ] |
| 7 | §7 | Owner values moved into the overlay | §1, §2, §5 | [ ] |
| 8 | §8 | Machine paths and personal names scrubbed from public files | §4, §7 | [ ] |

## 1. Overlay folder, config lookup and the rule that admits it

> **Started:** 2026-11-20T17:35:31Z

Bosun's rule is that it depends on no other repository. The overlay is Bosun's own private half, so the rule must say so before any code reads it, and every read goes through one helper so no script invents its own lookup order.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Admit the overlay in `AGENTS.md` and ignore it in `.gitignore`. Done when: `git check-ignore .private/x` prints the path and `AGENTS.md` names `rizonetech/Bosun-private` as Bosun's own overlay.
2. Add the lookup helper `scripts/lib/overlay.ps1` with its tests. Done when: `pwsh tests/overlay_test.ps1` passes and fails when the order is changed.

- [x] Add `.private/` to `.gitignore` (design, "How does it sit on the machine?"). Done when: `git check-ignore -v .private/config.toml` names the `.gitignore` line. Cheaper substitute: a per-clone `.git/info/exclude` entry, which a fresh clone would not have.
- [x] Amend the stand-alone rule in `AGENTS.md` so `rizonetech/Bosun-private`, cloned into `.private/`, is part of Bosun and not another repository, and the overlay holds data only, never code that wraps or replaces a public script. The rule that forbids cloning or copying another repository into the checkout says that this one overlay at `.private/` is the single exception, cloned and synced by Bosun's own scripts, and still forbids every other repository. Done when: the rule text names both, the clone rule states the exception and keeps the prohibition, and `pwsh scripts/check-all.ps1 -Only plan` passes. Cheaper substitute: leaving the rule as it is, so every later section breaks it. **Corrected 2026-11-20:** plan review gap 6, the clone prohibition would have refused `D00 T05 §2`.
- [x] Add `Get-BosunOverlayValue -Key <name> [-Variable <name>] [-Root <checkout>]` in `scripts/lib/overlay.ps1` that resolves a key from its `BOSUN_*` variable (default `BOSUN_` plus the key in upper case, so `vault_item` reads `BOSUN_VAULT_ITEM`), then `.private/config.toml` under `-Root` (default the checkout that holds the script, from `$PSScriptRoot`, never the caller's working directory), then returns a setup-required result naming the variable and the key. The variable is read first, and the file is opened only when the variable is unset, so a broken file never defeats an override. The result is an object with `Value`, `Source` (`variable`, `file` or `setup-required`) and `Message`. An empty variable counts as unset, and an empty value in the file (`vault_item = ""`) counts as missing: both end at `setup-required` with a null `Value` and a `Message` naming the variable and the key. Done when: the function returns the variable's value with `Source` `variable` when both are set. Cheaper substitute: reading `.private/config.toml` directly in each script. **Corrected 2026-11-20:** the parameters and the result shape were unnamed, which left each caller to invent them; plan review gaps 1 to 3 added the empty value, the default root and the read order.
- [x] Read `config.toml` as the flat subset the overlay needs: blank lines, `#` comment lines, and `key = "string"` pairs with TOML basic-string escapes (`\"`, `\\`, `\n`, `\t`, `\uXXXX`) or `key = 'literal'`, each optionally followed by a `#` comment after the closing quote. Values are taken as data: `$env:X`, `$(...)`, `#` and `=` inside the quotes stay as written. A table header, an array, a number, a duplicate key or any other line is refused, never skipped, and the whole file is read before any value is returned. Done when: each of those forms parses or is refused as stated in the test. Cheaper substitute: a regex that takes the first `=` and keeps the quotes. **Corrected 2026-11-20:** PowerShell has no TOML reader, and the plan did not say which TOML the overlay may hold; a full TOML parser would be code the overlay rule forbids it to need. Plan review gap 5 added the literal round trip and the trailing comment.
- [x] Make the helper refuse a malformed `config.toml` with the file, line and reason instead of returning nothing: it throws, and the message never includes the line's value. A `config.toml` that exists but cannot be read (held open exclusively by another process, access denied) also throws, naming the path and the reason; a `.private/` folder without `config.toml` is not an error and ends at `setup-required`. Done when: a broken file and a locked file in a test each produce their message, without the value, as a terminating error. Cheaper substitute: treating a parse or read failure as a missing key. **Corrected 2026-11-20:** "a non-zero result" was not defined for a function; a terminating error stops the calling script, which is the refusal wanted, and a value on a malformed line may be a secret. Plan review gap 4 added the unreadable file.
- [x] Add `tests/overlay_test.ps1`, each case in a temporary checkout under the scratch folder that the test removes, with `scripts/lib/overlay.ps1` copied into that checkout and called from a different working directory without `-Root`. Cases: variable first (the derived variable and the file both set to different values), a custom `-Variable` beating a differently valued derived variable, file second, setup-required third with no `.private/`, with `.private/` but no `config.toml`, and with the key missing from a present file, an empty variable, an empty file value, escapes and literal strings, a value holding `$env:PATH`, `$(1+1)`, `#` and `=` read back unchanged, a trailing comment, each refused form, a malformed line after the requested key, the same malformed file with the override set (its value) and unset (the refusal), and a locked file. Done when: it passes, and fails when the variable and file order is swapped in the helper. **Corrected 2026-11-20:** plan review gaps 1 to 5 named the cases a helper could pass while wrong.
- [x] Run `tests/overlay_test.ps1` as a step of the Windows scripts job in `.github/workflows/build.yml`; `scripts/check-all.ps1` already runs every `tests/*_test.ps1` it finds, and `tests/check_all_test.ps1` refuses a default-run test that CI does not run. Done when: `pwsh scripts/check-all.ps1 -List` shows `test:overlay`, `pwsh tests/check_all_test.ps1` passes, and the candidate's CI run shows the overlay step executed and passed. Cheaper substitute: a test that exists but no gate runs. **Corrected 2026-11-20:** check-all discovers tests by name (`scripts/check-all.ps1:78`), so there is nothing to register there; plan review gap 7 added the executed CI step as proof.
- [x] Commit: `overlay: ignored .private folder and one lookup order for owner values`

**Test checkpoint:** `pwsh tests/overlay_test.ps1` exits 0, and exits non-zero with the variable and file order swapped in `scripts/lib/overlay.ps1`, and again with the default root taken from the working directory.

> **Verified:** 2026-11-20 | §1 | d28016fc1707a524af9da0073c694f4d4776b8eb
> **Review:** `docs/reviews/d00-t05-s1.json`

## 2. Overlay clone in setup and pull after merge

> **Started:** 2026-11-20T18:27:26Z

The owner should never have to remember the overlay exists: a fresh clone gets it when the account can see it, and every pull of the public repository that merges or fast-forwards pulls it too (an already up to date pull, or a pull that rebases, runs no `post-merge` hook: `scripts/sync.ps1` in `D00 T05 §3` updates both in those cases). A stranger's clone sees neither step fail. **Corrected 2026-11-20:** plan review gap 1, "every pull" promised more than `post-merge` delivers.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Extend `scripts/setup-clone.ps1` with the overlay clone. Done when: on the owner's account `.private/.git` exists after a run, marked as the overlay, with its origin matching the source; and with access denied the run still exits 0 and says the overlay was skipped.
2. Add `scripts/hooks/post-merge`. Done when: a pull of the public repository fast-forwards a behind, clean, marked overlay and touches nothing else.

- [x] Add an overlay step to `scripts/setup-clone.ps1` that works on the resolved `-Repo` throughout (the lookup with `-Root $Repo`, the existence check, the clone and the read-back), and, when `<Repo>/.private` is absent, runs `gh repo view <owner/name>` and, when that succeeds, clones it with `gh repo clone <owner/name> <folder> -- ...` (the account's own protocol and credentials). Done when: a run on the owner's account leaves `.private/.git` and reads back its origin URL, and a run from another checkout with `-Repo` naming a folder with spaces gives only that folder an overlay. Cheaper substitute: cloning unconditionally, which fails every stranger's setup. **Corrected 2026-11-20:** plan review gap 3, the clone used the script's checkout, not `-Repo`.
- [x] Clone into a temporary folder in the checkout itself (`.private-clone-<id>`, ignored by `.gitignore`), so the move to `.private` is a rename on one volume even when the git data lives elsewhere (**Corrected 2026-11-20:** round 4 finding 2; it said under the checkout's git directory, which can be on another volume), check the clone's origin against the source (for `owner/name`, the HTTPS or SSH GitHub URL of that repository; for a local path, the same resolved path; for a URL, the same URL), mark it as the overlay (`git -C <clone> config bosun.overlay true`) and only then move it to `.private`. A failed clone, an origin that does not match, or an unwritable destination exits 1 with the reason and removes the temporary folder, so a retry starts clean and nothing half-made is ever called `.private`. Done when: a test with a probe that succeeds and a clone that fails exits 1, leaves no `.private`, and a retry with a working clone succeeds. Cheaper substitute: cloning straight into `.private` and calling a `.git` folder success. **Corrected 2026-11-20:** plan review gap 4.
- [x] Make the step skip with one plain line, exit 0, when `gh` is missing, not signed in, or refused, and leave an existing `.private` untouched: a marked overlay ("already present"), an unmarked checkout whose origin matches the source (marked then, and said), any other checkout, folder or file (named, never changed). Done when: each case prints its reason and the script exits 0. Cheaper substitute: an error a stranger has to read past. **Corrected 2026-11-20:** the design said to skip silently; one plain line is the default taken (a stranger learns why nothing happened, at the cost of one line), and the design is corrected to match. Plan review gaps 2 and 7.
- [x] Take the overlay's source from `BOSUN_OVERLAY_REPO` through `Get-BosunOverlayValue -Key overlay-repo -Root $Repo`, defaulting to `rizonetech/Bosun-private` when it is setup-required (the overlay's own `config.toml` cannot name it before the overlay exists). The supported forms are `owner/name`, an absolute local path to an existing repository, an `https://` URL without user information, and an `ssh://` or `git@host:path` URL; any other value (a leading `-`, user information in an HTTPS URL, anything else) is refused with exit 1 and a message that never repeats the value. A non-`owner/name` source is cloned with `git clone -- <source> <folder>` and needs no `gh`. Done when: setting the variable to a local bare repository changes the clone source in a test, and an HTTPS URL holding a synthetic credential is refused with the credential absent from the output. Cheaper substitute: a hard-coded name with no override. **Corrected 2026-11-20:** the test's local bare remote cannot pass `gh repo view`; the key is `overlay-repo` under `D00 T05 §1`'s grammar; plan review gap 8 named the source forms.
- [x] Print how to undo it after a clone: look at `git -C .private status` and `git -C .private log --branches --not --remotes` for local edits and unpushed commits, then delete `.private`; the public checkout works without it, and running setup again restores it. Done when: a test removes the overlay, a real public pull then succeeds without recreating it, and setup run again clones it back. **Corrected 2026-11-20:** plan review gap 9.
- [x] Add `scripts/hooks/post-merge` that pulls `.private` with `git -C .private pull --ff-only` only when it is a marked overlay (`bosun.overlay` is `true` in its config) and its working tree is clean (`git -C .private status --porcelain` prints nothing), and prints one line for every outcome (updated, already current, skipped as dirty, not a marked overlay, diverged, no upstream branch yet as for the empty overlay of 2026-11-20) without failing the merge. The hook clears `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_OBJECT_DIRECTORY`, `GIT_ALTERNATE_OBJECT_DIRECTORIES`, `GIT_COMMON_DIR` and `GIT_PREFIX` before it runs git in `.private`, so the pull acts on the overlay and never on the public repository. Done when: a test shows a behind clean overlay fast-forwarded, a dirty one (a local edit that does not overlap the incoming change) left with its HEAD, index and files as they were and its line printed, an unmarked checkout with an incoming commit left unchanged, and a diverged overlay left as it was. Cheaper substitute: a hook that silently swallows a failed pull. **Corrected 2026-11-20:** git exports `GIT_DIR` and friends to hooks, and `git -C` does not override them; plan review gaps 2 and 5 added the mark and the clean check.
- [x] Record `scripts/hooks/post-merge` as executable in git, and make `scripts/setup-clone.ps1` refuse any hook under `scripts/hooks` that is not recorded as `100755`, as it already does for `pre-commit`. Done when: `git ls-files -s scripts/hooks/post-merge` starts with `100755` and a test clone with a non-executable `post-merge` is refused. Cheaper substitute: relying on Windows, where the bit is ignored. **Corrected 2026-11-20:** the check named only `pre-commit`.
- [x] Add `tests/overlay_clone_test.ps1`, isolated from the operator's credentials and settings (its own `PATH` with a stand-in `gh`, `BOSUN_OVERLAY_REPO` and `GH_*` set or cleared per case, `HOME` and git's global config pointed at the test folder), covering: the default `owner/name` source through a successful stand-in `gh` backed by a local bare remote (probe, clone, origin read-back), a local path source with no `gh` at all, `gh` missing, signed out and refused each on its own, an existing `.private` (marked, matching, other checkout, plain folder, file), a probe that succeeds and a clone that fails then a retry, a refused source with a synthetic credential, the undo path, `-Repo` from another checkout into a folder with spaces, and the post-merge hook through a real `git pull` in a temporary clone with `core.hooksPath` set (behind, dirty, unmarked, diverged, no upstream, the public HEAD equal to the incoming commit), plus the hook run directly with `GIT_DIR` set to the public repository, both HEADs asserted exactly. Make `tests/setup_clone_test.ps1` isolated the same way, so its temporary clones never reach the real overlay. Done when: both pass, `overlay_clone_test.ps1` fails if the existing-folder guard is removed, and fails if the hook stops clearing `GIT_DIR`. **Corrected 2026-11-20:** plan review gaps 6 and 7.
- [x] Run `tests/overlay_clone_test.ps1` in the Windows scripts job of `.github/workflows/build.yml` (`scripts/check-all.ps1` finds it by name). Done when: `-List` shows `test:overlay_clone`, `pwsh tests/check_all_test.ps1` passes, and the candidate's CI run shows the step executed and passed. **Corrected 2026-11-20:** check-all discovers tests (`scripts/check-all.ps1:78`); plan review gap 10.
- [x] Correct `docs/specs/2026-11-20-public-core-private-overlay-design.md` ("Setup" and "Pull") to the one-line skip, the overlay mark and the pulls `post-merge` covers. Done when: the design's lines say so.
- [x] Commit: `overlay: setup clones the overlay and every pull keeps it current`

**Test checkpoint:** `pwsh tests/overlay_clone_test.ps1` exits 0, and fails when the existing-folder guard in `scripts/setup-clone.ps1` is removed, and when `scripts/hooks/post-merge` stops clearing `GIT_DIR`.

> **Verified:** 2026-11-20 | §2 | 66e6953e9a9f1ce417d0d65ae1bd5e41e5d4d31b
> **Review:** `docs/reviews/d00-t05-s2.json`

## 3. One sync command for both repositories

> **Started:** 2026-11-20T20:31:43Z

The owner needs one place to see whether anything is waiting: one command that pulls and pushes both repositories and prints their state, so nothing is left unsynced by accident. It is also how both are updated when a public pull runs no `post-merge` hook (already up to date, or a rebase; `D00 T05 §2`).

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add `scripts/sync.ps1` with `-Check`. Done when: it prints one line per repository with its state, changes nothing, and exits 1 when anything needs attention.
2. Make a plain run update then push both, all or nothing. Done when: a behind and ahead pair ends level in a test, with the exact commits on each side.

- [x] Move `Get-OverlayCloneFault`, `Get-OverlaySource` and `Test-OverlayOrigin` from `scripts/setup-clone.ps1` into `scripts/lib/overlay.ps1` and call them from both, so setup and sync judge the overlay and its source by the one rule `D00 T05 §2` settled on, alongside the `bosun.overlay` mark read with `--local`. Done when: `tests/overlay_clone_test.ps1` still passes and sync refuses a linked, worktree or unmarked `.private` in a test. Cheaper substitute: a second, looser check in sync. **Corrected 2026-11-20:** the plan predates `D00 T05 §2`, whose reviews found five ways a `.private` can belong to another checkout.
- [x] Take both checkouts from the script's own location (the public checkout is the one holding `scripts/sync.ps1`, the overlay its `.private`), never the working directory, and clear `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_OBJECT_DIRECTORY`, `GIT_ALTERNATE_OBJECT_DIRECTORIES`, `GIT_COMMON_DIR` and `GIT_PREFIX` before the first git call. Done when: a test runs it from another dirty checkout, and with `GIT_DIR` pointing at another repository, and that repository's files, index, HEAD and remote refs are unchanged. **Corrected 2026-11-20:** plan review gap 3.
- [x] Add `scripts/sync.ps1 -Check` that fetches each repository's upstream remote with `git fetch --quiet --prune` and prints one line each, `public: <branch>, <state>` and `overlay: <branch>, <state>`, where the state is `level and clean`, or the parts that apply of `ahead N`, `behind N`, `diverged (ahead N, behind N)` and `uncommitted changes` (untracked files included, whatever `status.showUntrackedFiles` says), or one of `no upstream branch`, `upstream branch gone` (its remote branch was deleted), `detached HEAD`, `remote unreachable`, `not present` (no `.private`), `not the overlay (<reason>)`, `push goes elsewhere` (an overlay whose remote's fetch or push URL is not the overlay source), `empty, first commit waiting on <branch>` or `empty, no commit yet`. A git command that fails (an unreadable index, a status that cannot be read) is a state of its own, `could not be read (<operation>)`, never taken for clean. No line prints a remote URL or a message the remote sent. `-Check` changes no local branch, file, index, tag, `FETCH_HEAD` or ref on either remote; it does update the remote-tracking refs (`refs/remotes/...`), as any `git fetch` does, since reporting newly fetched counts needs them (**Corrected 2026-11-20:** round 8 read "remote ref" as the tracking refs). Done when: the output matches a test fixture for each state, and the refs, files and index of both checkouts and both bare remotes are the same before and after. Cheaper substitute: wrapping `git status` text the user has to read. **Corrected 2026-11-20:** the states an owner meets were unnamed; plan review gaps 1, 2, 4, 6 and 8.
- [x] Make `-Check` exit 1 when either repository is anything but `level and clean` (an absent overlay counts as clean: a stranger's clone has none), and 0 when both are. Done when: tests assert each exit code.
- [x] Make a run without `-Check` all or nothing: it reads both states first, and when either is in a state it does not act on (diverged, uncommitted changes, detached HEAD, no upstream, upstream gone, remote unreachable, not the overlay, push goes elsewhere, could not be read) it changes neither and exits 1. Otherwise, overlay first: one that is behind is fast-forwarded with `git merge --ff-only --no-overwrite-ignore @{u}`, one that is ahead is pushed to its own upstream branch only (`git push <remote> HEAD:<upstream branch>`, which may have another name), an empty one whose first commit is waiting takes it (`git checkout --no-overwrite-ignore --track <remote>/<default branch>`, as the hook does); then the public checkout the same way. The public fast-forward runs `scripts/hooks/post-merge`, which finds the overlay already dealt with (all or nothing means sync never fast-forwards the public checkout while it refuses the overlay), and its output is not shown, so each repository still gets one line. **Corrected 2026-11-20:** a `BOSUN_SYNC` variable for the hook to stand down was planned; removing it broke no case, because the all-or-nothing rule already keeps the hook from acting on an overlay sync refused. A merge or push that is refused stops the run with the repository, the operation and a fixed reason (never git's or the remote's own text), after saying what was already done. Every run ends with the two `-Check` lines read again and exits as `-Check` would. Done when: a test ends with both repositories level and the exact incoming and outgoing commits on each side, both directions for each repository, an upstream branch named differently from the local one, a diverged repository beside an actionable one leaves both unchanged, an ignored file an incoming change would replace is kept, a second push refused after a first succeeded says so and a retry after the cause is gone finishes, and an empty overlay gets its first commit with the public checkout already current. Cheaper substitute: `git pull` (which replaces ignored files), or a merge or rebase the user did not ask for. **Corrected 2026-11-20:** `D00 T05 §2` round 3 showed a plain pull replaces an ignored file; plan review gaps 2, 5, 6, 7 and 8 set the all-or-nothing rule, the first commit, the hook and the refusals.
- [x] Never commit, stash or stage on the user's behalf: a dirty repository is reported, not changed. Done when: a dirty, ahead repository's files, index, HEAD and remote ref are unchanged after a run. Cheaper substitute: an automatic `git commit -a`.
- [x] Add `tests/sync_test.ps1` with local bare remotes for both repositories, distinct known commits, the real `scripts/hooks/post-merge` installed, and isolated like `tests/overlay_clone_test.ps1` (its own `HOME`, git config and `PATH`), plus a public-only clone through both modes (a real public update, `.private` still absent). Done when: it passes and fails if `-Check` returns 0 for a dirty overlay. **Corrected 2026-11-20:** plan review gaps 6, 7 and 9.
- [x] Run `tests/sync_test.ps1` in the Windows scripts job of `.github/workflows/build.yml` (`scripts/check-all.ps1` finds it by name), and document `scripts/sync.ps1` in `README.md` under a new question-shaped section, "How do I keep my checkout and its overlay in sync?", with `-Check`, the exit codes, what a refusal means and what to do about each state. Done when: `-List` shows `test:sync`, `pwsh tests/check_all_test.ps1` passes, the candidate's CI run shows the step executed and passed, and the README section covers those four. **Corrected 2026-11-20:** check-all discovers tests by name, the README has no development setup section; plan review gap 9.
- [x] Commit: `overlay: one command shows and syncs the public repo and its overlay`

**Test checkpoint:** `pwsh tests/sync_test.ps1` exits 0, and fails when `-Check` is made to ignore a dirty overlay, and when the all-or-nothing rule is removed.

> **Verified:** 2026-11-21 | §3 | 2bc08643ec489ce502b462acc7a163759acff5f0
> **Review:** `docs/reviews/d00-t05-s3.json`

## 4. Pre-push leak guard with gitleaks and the private deny list

> **Started:** 2026-11-20T23:41:22Z

Making the repository public was the irreversible step; from here every push is a publish. The guard refuses a push before it leaves the machine when the outgoing commits contain a secret or a string the owner listed as private, and the list itself stays in the overlay.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add `scripts/leak-scan.ps1` over a set of outgoing commits. Done when: it exits 1 naming the commit, the place (file and line, message, path, tag) and the rule (never the matched text) for a planted `gitleaks` hit and for a deny-list hit.
2. Call it from `scripts/hooks/pre-push`. Done when: a push of a commit with a planted hit is refused, the remote's refs read back unchanged, and a clean push goes through.

- [x] Install `gitleaks` per user with `winget install --id Gitleaks.Gitleaks -e --version 8.30.1 --scope user` and record the version and command in the README's build section, beside the other tools, with the Linux and WSL install from the release archive. Done when: `gitleaks version` prints `8.30.1` and the README names both. Cheaper substitute: a scan that silently passes when the tool is missing. **Corrected 2026-11-21:** `docs/development.md` does not exist; the README's build section is the setup list; plan review gap 8 added WSL. Installed 2026-11-21 (`gitleaks version` -> `8.30.1`).
- [x] Add `scripts/leak-scan.ps1 -Remote <name or URL>`, reading the hook's ref lines from standard input (or `-Updates <line>...`), that works out the outgoing commits from the destination itself: `git ls-remote` on the remote gives its current tips, and the outgoing set is `git rev-list <local shas> --not <the remote tips present locally>` (a local tracking ref is never trusted; an empty remote makes every commit outgoing; a remote that cannot be listed refuses). Done when: a stale tracking ref and a first push to an empty remote with the hit in the root commit are both caught. **Corrected 2026-11-21:** plan review gap 3, a tracking ref can outlive the remote branch; repair cycle 1 plan review gap 6: the parameter is `-Updates`, not `-Push`.
- [x] Run `gitleaks git --log-opts <the outgoing commits, merges diffed against their first parent> --redact --no-banner --report-format json --report-path -` (the report read from its standard output, held in memory, never written to disk) and exit 1 on any finding, printing commit, file, line and rule only, read from the JSON report; a missing or malformed report refuses. Done when: a test commit holding a token pattern built at run time (so no token-shaped literal is ever committed) is reported with nothing of the token in either output stream, and a hit introduced only by a merge resolution is caught. **Corrected 2026-11-21:** a token literal in a test would be refused by GitHub push protection and flagged by `D00 T05 §6`; plan review gaps 1 and 4; repair cycle 1 plan review gap 1: the report is in memory.
- [x] Add the deny-list pass: every non-empty, non-`#` line of `.private/deny-list.txt` (UTF-8, compared literally and case-insensitively, never as a pattern) is matched against what the outgoing commits publish: the lines they add (merges diffed against their first parent, so a resolution is covered), the paths they add or rename to, their messages, and the names and annotations of tags being pushed. A hit prints commit, place and line number but never the line, the entry or a path that holds it (such a path is shown as `a path`). Done when: tests refuse a listed string in added content at a known non-first line, in a renamed path, in a message, in a merge resolution and in a tag annotation, each with the exact commit and place and neither output stream holding the string; mixed case and regex punctuation match literally, a near-match passes, and blank and `#` lines are ignored. Cheaper substitute: printing the matched entry, which leaks the private list into terminals and logs. **Corrected 2026-11-21:** plan review gaps 1, 2 and 9.
- [x] Make the scan refuse, not pass, on every failure: `gitleaks` missing (with the install command for the environment running it: winget on Windows, the release archive on Linux and WSL), a deny list that exists but cannot be read, a git command that fails or a remote that cannot be listed. Done when: a test for each exits 1 with its reason, and through the hook the remote's refs read back unchanged. **Corrected 2026-11-21:** plan review gaps 4 and 8.
- [x] Add `scripts/hooks/pre-push` that hands the pushed refs from standard input (`<local ref> <local sha> <remote ref> <remote sha>`) and the remote to `scripts/leak-scan.ps1`, skipping deletions, scanning every pushed ref and every commit not already on the destination, and refusing the push on a finding or a failed scan. Overlay pushes need no exception: they run inside `.private`, whose hooks are its own. Done when: real pushes to a local bare remote refuse a hit in an intermediate commit later removed at the tip, and a multi-ref push whose second ref holds the hit, with every destination ref read back unchanged; a clean push goes through. Cheaper substitute: scanning only the last commit or the first ref. **Corrected 2026-11-21:** the plan's overlay skip was moot; plan review gap 5.
- [x] Carry the guard's own lines (each starts `leak-scan:` and holds no remote text) through `scripts/sync.ps1` when its push is refused, so the owner sees the finding or the missing-tool remedy. Done when: a guarded sync against a bare remote prints the guard's reason and the public ref is unchanged. **Corrected 2026-11-21:** plan review gap 7.
- [x] Record `scripts/hooks/pre-push` as executable in git, add it to the hooks `scripts/setup-clone.ps1` requires, and give the fixtures of `tests/setup_clone_test.ps1` and `tests/overlay_clone_test.ps1` an executable `pre-push`, with a case that removes only it. Done when: `git ls-files -s scripts/hooks/pre-push` starts with `100755`, setup refuses a checkout without it, and both suites pass. **Corrected 2026-11-21:** plan review gap 6.
- [x] Add `tests/leak_scan_test.ps1`, isolated like the other overlay suites, with the cases above, a clean range, a missing deny list (the scan still runs `gitleaks`), and a deletion push. Done when: it passes and fails if the deny-list pass is removed.
- [ ] Read the deny list by one line grammar where `scripts/leak-scan.ps1` builds `$deny`: a line is a comment only when its first character is `#`, blank when it holds only whitespace, and otherwise an entry, matched as its text with surrounding whitespace removed (so an indented `  # name` line is an entry, and a stray space never narrows a match). Done when: `tests/leak_scan_test.ps1` lists `  # name  ` (leading and trailing spaces) and refuses a push adding `# name` with neither, the destination ref read back unchanged; a separate clean push still ignores a column-0 `#` line and a whitespace-only line; and the test fails when the comment test reads the trimmed line or when the entry keeps its spaces. **Corrected 2026-11-21:** repair cycle 1; round 33 found the comment test ran on the trimmed line, so an indented literal entry was dropped; its plan review gap 2 asked for the spaces only on the list side.
- [ ] Judge every gitleaks result by one rule in `Read-GitleaksReport` of `scripts/leak-scan.ps1`, used by the commit scan and by `Find-SecretsIn`: an exit other than 0 or 3 refuses, a report that is not a JSON array refuses, exit 3 with no entry refuses, and exit 0 with an entry refuses. Done when: `tests/leak_scan_test.ps1` runs the hook with a stand-in `gitleaks` first on `PATH` (a script that prints a chosen report and exits with a chosen code) for exit 0 with no report, exit 3 with `[]`, and exit 0 with an entry, on both the commit scan and an object scan, each refused with the destination ref unchanged. **Corrected 2026-11-21:** repair cycle 1 plan review gap 1.
- [ ] Prove the Linux and WSL missing-tool remedy: `tests/leak_scan_linux_test.ps1` runs the hook in a scratch checkout on Linux with `gitleaks` absent from `PATH` and checks the release-archive remedy and the unchanged destination ref, and the `linux` job of `.github/workflows/build.yml` runs it with `pwsh`. Done when: the candidate's CI run shows the step executed and passed, and the test fails when the remedy names `winget`. **Corrected 2026-11-21:** repair cycle 1 plan review gap 3.
- [ ] Check each deny-list refusal in `tests/leak_scan_test.ps1` (renamed path, message, merge resolution, tag annotation) against the expected commit or tag object id and place in the combined output, with the listed string absent from it. Done when: each case captures its object id from git and fails when the scan reports another object or prints the string. **Corrected 2026-11-21:** repair cycle 1 plan review gap 4.
- [ ] Make the deletion-push case in `tests/leak_scan_test.ps1` prove its setup: the branch's creation push exits 0 and the destination reads back the expected tip before the deletion is pushed. Done when: the case fails if the creation push is refused. **Corrected 2026-11-21:** repair cycle 1 plan review gap 5.
- [ ] Add `-Range <revision range>` and `-All` to `scripts/leak-scan.ps1` for the scans `D00 T05 §7` and `§8` run outside a push: `-Range` scans the commits `git rev-list <range>` gives, and `-All` every commit reachable from `HEAD`, root commit included; neither reads standard input or lists a remote. Done when: `tests/leak_scan_test.ps1` refuses a deny-list hit in a root commit with `-All` and one inside a range with `-Range`, and passes a clean range. **Corrected 2026-11-21:** repair cycle 1 plan review gap 6; `§8`'s `<root>..HEAD` would leave the root commit out, so it uses `-All`.
- [ ] Run `tests/leak_scan_test.ps1` in the Windows scripts job of `.github/workflows/build.yml`, with `gitleaks` 8.30.1 installed in that job from its release archive, checked against the release's `checksums.txt`. Done when: `-List` shows `test:leak_scan`, `pwsh tests/check_all_test.ps1` passes, and the candidate's CI run shows the step executed and passed. **Corrected 2026-11-21:** check-all discovers tests by name, and CI has no `gitleaks`.
- [x] Commit: `overlay: refuse a push that would publish a secret or a private string`

**Test checkpoint:** `pwsh tests/leak_scan_test.ps1` exits 0, and fails when the deny-list pass in `scripts/leak-scan.ps1` is removed.

> **Repair cycle:** 1: 2026-11-21: its 33 review rounds ran out with a P1 open (round 33: deny-list lines are trimmed before the # comment test, so an indented literal entry is lost); the new cycle fixes it under a fresh plan review (after: its 33 review rounds ran out with findings open)

## 5. Agent rules split into public and overlay halves

Agents need the owner's rules on the owner's machine and must not see them anywhere else. The public files keep every rule a contributor needs and load the overlay's rules when present.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Prove how Claude Code handles a missing `@` import. Done when: the result is written in this section with the Claude Code version.
2. Wire the import or the fallback, and the pointer for the other agents. Done when: a session in the owner's checkout lists `.private/AGENTS.md` among its loaded memory, and a checkout without it starts cleanly.

- [ ] Test `@.private/AGENTS.md` in `CLAUDE.md` in a scratch checkout with and without the file, and record whether a missing import is silent. Done when: the finding and `claude --version` are recorded under this section.
- [ ] Add the import to `CLAUDE.md` if a missing import is silent; otherwise make `scripts/setup-clone.ps1` write an ignored `CLAUDE.local.md` holding the import when `.private/AGENTS.md` exists. Done when: the chosen path loads the overlay rules in the owner's checkout. Cheaper substitute: copying the owner's rules into `CLAUDE.md`.
- [ ] Add one line to `AGENTS.md` telling Codex and Antigravity to also read `.private/AGENTS.md` when it exists. Done when: the line is present and `GEMINI.md` and `CODEX.md` do not repeat it. Cheaper substitute: a copy of the rules in each agent file.
- [ ] Add `CLAUDE.local.md` to `.gitignore` if the fallback is used. Done when: `git check-ignore CLAUDE.local.md` prints it.
- [ ] Add `tests/agent_rules_test.ps1` asserting `CLAUDE.md` and `AGENTS.md` carry the overlay pointer and no `op://`, vault or machine path text. Done when: it passes and fails when an `op://` line is added to `AGENTS.md`.
- [ ] Register `tests/agent_rules_test.ps1` in `scripts/check-all.ps1` and CI. Done when: `-List` shows it.
- [ ] Commit: `overlay: agent rules load the overlay's half when present`

**Test checkpoint:** `pwsh tests/agent_rules_test.ps1` exits 0, and fails when an `op://` line is added to `AGENTS.md`.

## 6. Full-history secret scan in CI

The local guard protects pushes from the owner's machine; CI protects everything else, including history pushed before the guard existed and pushes from another machine. It never sees the overlay, so it scans for secrets only.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add a `gitleaks` job to `.github/workflows/build.yml` over the full history. Done when: the job is green on `main`, and the same job, given a synthetic commit holding a token built inside the job, reports it. **Corrected 2026-11-21:** `D00 T05 §4`'s pre-push guard refuses a pushed planted token, so the red proof happens inside CI.

- [ ] Add a `secrets` job on `ubuntu-latest` to `.github/workflows/build.yml` with `fetch-depth: 0` that runs a pinned `gitleaks` release over the whole history with `--redact`. Done when: the job runs on the next push. Cheaper substitute: scanning only the pushed commit.
- [ ] Pin the `gitleaks` download by version and SHA-256 in the workflow. Done when: the step verifies the checksum before running. Cheaper substitute: an unpinned `latest` download.
- [ ] Prove the job finds a secret without pushing one: a step in the same job makes a throwaway commit in a temporary clone holding a token built at run time, runs the same pinned `gitleaks` over it and asserts a finding (and that the token appears nowhere in the log). Done when: the job is green on `main` with that self-test step passing, and the step fails when the scanner is skipped. **Corrected 2026-11-21:** the plan review of `D00 T05 §4` found the pushed throwaway branch would be refused by that section's guard.
- [ ] Add `.gitleaksignore` entries only for known false positives with a comment naming each, starting empty. Done when: the job is green on `main` with every ignore entry explained.
- [ ] Commit: `ci: scan the full public history for secrets on every push`

**Test checkpoint:** `python scripts/ci_status.py` reports the `secrets` job green on HEAD, with its self-test step finding the synthetic token.

## 7. Owner values moved into the overlay

With the plumbing in place, the owner's values leave the public files: each becomes a key in `.private/config.toml` read through `Get-BosunOverlayValue`, and the public default becomes a setup-required message.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Seed the overlay repository. Done when: `rizonetech/Bosun-private` holds `config.toml`, `AGENTS.md`, `deny-list.txt` and a `README.md` that says what belongs there.
2. Switch each owner value to the helper. Done when: `git grep -n "op://" -- . ':!docs/reviews'` prints nothing and the owner's `store-claude-token.ps1 -FromVault` still works.

- [ ] Seed `.private/README.md` stating that the overlay holds data only and how it is cloned and synced. Done when: it is pushed to `rizonetech/Bosun-private`.
- [ ] Move the `op://` item default from `scripts/store-claude-token.ps1` to `vault_item` in `.private/config.toml`, read through `Get-BosunOverlayValue` with `BOSUN_VAULT_ITEM` as its variable. Done when: the script prints a setup-required message without the overlay and imports the token with it. Cheaper substitute: an empty default that fails later with a vague error.
- [ ] Replace the `op://` item in `tests/start_run_test.ps1` with a sample value. Done when: the test passes and `git grep -n "op://" -- tests` prints only sample values.
- [ ] Move owner-only rules (the 1Password Conclave vault, the desktop shortcuts' owner details, and any rule that names the owner's accounts) from `AGENTS.md` to `.private/AGENTS.md`, keeping a generic public rule where a contributor needs one. Done when: `AGENTS.md` has no vault or account name and `.private/AGENTS.md` holds them. Cheaper substitute: deleting the rules, so the owner's agents lose them.
- [ ] Replace vault wording in `scripts/start-run.ps1` messages with the generic token-store wording. Done when: `git grep -n "1Password" -- scripts` prints nothing outside comments that name it as one supported store.
- [ ] Seed `.private/deny-list.txt` with the private strings found in this file's current state (owner name, private client and project names, vault and item names) and push it. Done when: `scripts/leak-scan.ps1 -Range origin/main~20..origin/main` runs the deny-list pass with a non-empty list.
- [ ] Leave `CHANGELOG.md` entries that are released history as written, and word new entries generically. Done when: the decision is noted under this section.
- [ ] Commit: `overlay: owner values and rules read from the private overlay`

**Test checkpoint:** `pwsh tests/agent_rules_test.ps1` and `pwsh tests/overlay_test.ps1` exit 0, and `git grep -n "op://" -- . ':!docs/reviews'` prints only sample values.

## 8. Machine paths and personal names scrubbed from public files

The last private details in the public tree are machine paths and a personal name. Live files cite repository-relative paths or placeholders; stamped sections, phase-run records and review receipts are history and stay as written.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Replace machine paths in live files. Done when: `git grep -lE "[A-Z]:\\\\(GitHub|scratch|Bin)" -- AGENTS.md README.md scripts src tests docs/specs` prints only lines inside dated history notes.
2. Prove the tree is clean with the guard. Done when: `scripts/leak-scan.ps1` over the full history reports no `gitleaks` finding and the deny-list pass over live files is clean.

- [ ] Replace machine paths in code comments in `scripts/lib/build-cache.ps1` and `scripts/lib/plugin-install.ps1` with descriptions that name no drive or folder. Done when: neither file matches a drive-letter path. Cheaper substitute: deleting the comments and losing the reason they record.
- [ ] Replace machine paths in tests (`scripts/tests/test_dispatch_hook.py`, `tests/build_cache_test.ps1`, `tests/test_core.cpp`) with paths built from the test's temporary folder. Done when: each test passes and none names a real drive folder.
- [ ] Replace the personal name in `tests/Bosun.Panel.Tests/Controls/QueueVisualTests.cs` with a sample name. Done when: the test passes and the name is gone from tracked files outside `docs/reviews/`.
- [ ] Replace machine paths in open (unstamped) sections of `todo/`, in `docs/specs/` and in `docs/acceptance/preflight.md` with repository names, leaving stamped sections and `docs/phase-runs/` as written. Done when: `python scripts/todo-graph.py validate` passes and no open section names a drive path.
- [ ] Review sibling repository names in `design/mockups/control-panel.html` and `docs/images/` and replace private client and project names with samples (`D99 T01 §4`). Done when: the deny-list pass over those files is clean.
- [ ] Run `scripts/leak-scan.ps1` over the full history and record the result in `D99 T01 §4`'s gitleaks item. Done when: no finding remains, or each is a recorded false positive in `.gitleaksignore`.
- [ ] Commit: `overlay: scrub machine paths and personal names from the public tree`

**Test checkpoint:** `pwsh scripts/leak-scan.ps1 -All` exits 0 on the owner's checkout with the overlay present. **Corrected 2026-11-21:** was `-Range <root>..HEAD`, which leaves the root commit out (`D00 T05 §4` repair cycle 1 plan review gap 6).

## Verification

- With `.private/` removed, `pwsh scripts/check-all.ps1` passes: a stranger's clone builds and passes every gate.
- With `.private/` present, `pwsh scripts/sync.ps1 -Check` exits 0 after `pwsh scripts/sync.ps1`, and a push of a planted deny-list string is refused.
- CI's `secrets` job is green on HEAD.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
