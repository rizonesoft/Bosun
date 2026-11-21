# Public core and private overlay

**Status:** approved design, not built. **Owner decisions:** 2026-11-20. **Supersedes:** the 2026-11-18 plan to export a clean public copy from a private source.

## What changed and why

On 2026-11-20 Bosun went public with its full history and moved from `rizonetech/Bosun` to `rizonesoft/Bosun`. The owner then reversed the earlier plan: development happens on the public repository, and a small private overlay holds only the owner's machine and account specifics. One codebase means no drift between a private and a public version, and nothing ever waits to be integrated.

Decisions recorded with this spec:

- Public `rizonesoft/Bosun` is where all code, plans and review receipts live.
- One private overlay per repository: `rizonetech/Bosun-private`. Bosun never reads another project's overlay, which keeps the rule that Bosun stands alone.
- `todo/` and `docs/reviews/` stay public, scrubbed of machine paths and personal names. Stamped history is left as written; open sections cite by repo name.
- Fresh history is dropped: the full history was public from 2026-11-20 13:29 UTC, and a scan of every commit for provider tokens and private keys found none.

## How does it sit on the machine?

```
D:\GitHub\Bosun\            clone of rizonesoft/Bosun (public, all code)
  .gitignore                ignores .private/
  .private\                 clone of rizonetech/Bosun-private
    config.toml             owner values the code reads at run time
    AGENTS.md               owner-only agent rules for this repo
    deny-list.txt           strings that must never reach the public repo
```

The overlay is a nested clone in an ignored folder, never a submodule (a submodule would publish the private repo's name and break a stranger's clone) and never a fork (a fork drifts).

## How does the code find the overlay?

The overlay holds data, never code. Code that needs an owner value resolves it in this order, which `AGENTS.md` already requires (explicit config, then discovery, then a setup-required state):

1. The tool's own `BOSUN_*` environment variable, for one-off overrides.
2. `.private/config.toml` in the checkout, when present.
3. A setup-required state that names the variable or key to set. A stranger's clone always lands here, and it must build and pass CI here.

If the owner's setup needs behaviour the public code lacks, the public code gains a setting and the overlay sets it. An overlay script that wraps or replaces a public script is a design fault.

## What moves into Bosun's overlay?

Measured 2026-11-20. Bosun is already close to public-clean.

| Item | Today | After |
| ---- | ----- | ----- |
| Vault item path | default in `scripts/store-claude-token.ps1` | `vault_item` in config |
| Vault wording | `AGENTS.md`, `CHANGELOG.md`, `start-run.ps1` | generic token store text |
| Owner AGENTS rules | mixed into `AGENTS.md` | `.private/AGENTS.md` |
| Machine paths | 26 files, mostly docs and comments | repo names or placeholders |
| Personal name | `QueueVisualTests.cs` fixture | sample name |
| Reviewer setup | `.conclave/reviewers.toml` | stays public if generic |
| `.remember/` | untracked | moves to `.private/` |

Paths inside stamped sections, phase-run records and `docs/reviews/` receipts are history and stay as written.

## How does it stay in sync without thinking about it?

- **One folder.** Work in `D:\GitHub\Bosun` as before. VS Code and lazygit list the nested `.private` repo next to the main one.
- **Setup.** `pwsh scripts/setup-clone.ps1` clones `rizonetech/Bosun-private` (or `BOSUN_OVERLAY_REPO`) into `.private` when `gh repo view rizonetech/Bosun-private` succeeds, checks the clone's origin and marks it as the overlay (`bosun.overlay = true` in its config). Otherwise it says why in one line and goes on (corrected 2026-11-20 from "skips it silently": a stranger learns why nothing happened). An existing `.private` is never changed, except that an unmarked checkout of the overlay itself is marked.
- **Pull.** A `post-merge` hook in `scripts/hooks` fast-forwards a marked, clean `.private` after every pull of the public repo that merges or fast-forwards. An already up to date pull or a pull that rebases runs no `post-merge` hook; `scripts/sync.ps1` updates both then.
- **Status and push.** `pwsh scripts/sync.ps1` pulls and pushes both repos and prints one line each: clean, ahead, behind or dirty. `-Check` only reports.
- **Leak guard.** A `pre-push` hook scans the commits being pushed with `gitleaks` and with every line of `.private/deny-list.txt`, and refuses the push on a hit. GitHub secret scanning with push protection (enabled 2026-11-20) is the second line. The deny list itself stays private.
- **Agent rules.** The overlay's rules are trusted only from the marked overlay's own clone, never from whatever folder sits at `.private`. The public `AGENTS.md` tells every agent to follow `.private/AGENTS.md` only when `.private` is a real folder with its own `.git` and `bosun.overlay` is `true`. For Claude Code, a `SessionStart` hook (`scripts/overlay-rules-hook.ps1`, through `Get-BosunOverlayRules` in `scripts/lib/overlay.ps1`) checks the overlay at every session start and hands the file to the session as context, writing nothing. A missing import proved silent in Claude Code 2.1.330 (`D00 T05 §5`), but a plain import in `CLAUDE.md` would load an unrelated `.private`; a managed import in `CLAUDE.local.md` was built first and replaced, since its file went stale between the maintaining steps and rewriting the owner's own file kept costing fixes. **Corrected 2026-11-21:** this said the public `CLAUDE.md` imports the file and the other agents read it whenever it exists.

## What runs in CI?

CI runs only on the public repo and never sees the overlay, so every green run proves a stranger's clone builds and passes. CI adds a `gitleaks` history scan. The overlay has no CI and costs nothing.

## Build order

1. Overlay plumbing: `.gitignore`, config lookup helper, `setup-clone.ps1` clone step, `post-merge` hook, `sync.ps1`, `pre-push` leak guard, agent rule import. Tests for each with and without `.private`.
2. Create `rizonetech/Bosun-private`, move the owner values and rules into it, and seed `deny-list.txt`.
3. Scrub the items in the table, then close the open checks in `D99 T01 §4`.
4. Conclave-SDK follows with its own spec in its own repo (about 100 files mention the vault and about 40 TickTick or Outlook, so its overlay is larger).

## Reversal cost

Low. Deleting `.private/` and the hooks returns Bosun to a plain public repo; the code already runs without the overlay by design. Making the public repo private again is possible, but anything already cloned or cached stays public.
