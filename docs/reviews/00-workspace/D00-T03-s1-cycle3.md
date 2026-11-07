## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:18:41Z
- Candidate: 555dd884430d781bf4d182109504328ec9e77cc3
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 555dd884430d781bf4d182109504328ec9e77cc3
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: 1cf733edef32bdeb

1. **P1, lines 68-74 and 79:** A proven path-filter skip does not prove inherited code passed. After B fails or becomes unverified, a Markdown-only C can be `skipped` against B, return `proceed`, and clear the repair budget despite retaining B’s broken code. Require successful evidence for the unchanged workflow inputs before treating a skip as gate success. Add cross-process tests for failed and unverified B followed by Markdown-only C, asserting neither clears the unresolved failure.

2. **P1, lines 73, 77 and 79:** The previously gated commit is not necessarily the previous remote head. A crash after pushing B but before gating it leaves A recorded; a later push C that reverts B and edits Markdown can incorrectly appear Markdown-only against A. The proposed test gates B, so it misses this crash window. Persist the actual push boundary before pushing, or recover authoritative push evidence; otherwise refuse to infer a skip. Test the push-before-gate crash across separate processes and reconcile the conflicting base instructions.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: a skip now means inputs unchanged since the last proven commit, so a failed or unverified commit can never be skipped over (P1) in the D00 T03 §1 contract, this commit.
- Planned: no push boundary is needed: a crash between push and gate can't hide code, because the diff from the proven commit still shows it (P1) in the D00 T03 §1 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:21:50Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 555dd884430d781bf4d182109504328ec9e77cc3
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 8
- Digest: b12cfa665f42f42d

### adversarial: needs-attention

1. **P1, `scripts/ci_status.py:462-467`: Explicit `--base` bypasses proven-commit protection.** Reproduce with A green, B introducing failing code, then C changing only `README.md`. Run `--gate --sha C --base B --json`, as the documented CLI permits. With no runs for C, both workflows become `skipped`, the gate returns `proceed`, and B’s repair history is cleared despite its broken code remaining. Using the repository’s workflow filters, an in-memory reproduction returned exit 0 with `--base B`, versus exit 5 without it. Gate skip evidence must use a proven base even when `--base` is supplied.

2. **P2, `scripts/ci_status.py:446-451`: A commit-resolution timeout produces a usage error instead of a gate action.** Run `--gate --sha HEAD --wait-minutes 0.1 --json` when disk contention or process startup makes Git exceed six seconds. `resolve_commit` converts `Unavailable` into `None`, and `parser.error` exits 2, incorrectly claiming HEAD is invalid and emitting no JSON or action. Reproduced by injecting the timeout exception raised by the tool wrapper. Distinguish unavailable resolution from an invalid commit and return the gate’s unavailable result.

HIGHEST: P1
OVERALL: needs-attention

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:23:25Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 555dd884430d781bf4d182109504328ec9e77cc3
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: f56d0be41347428c

### consistency: needs-attention

1. **P3, `todo/00-workspace/TODO-03-campaign-readiness.md:73`: The checked policy still instructs passing the previous remote head to `--gate`.** The candidate now rejects `--gate --base`, while the process-phase skill correctly omits it. Reproduce by following that checklist instruction: `python scripts/ci_status.py --gate --sha HEAD --base HEAD^ --json` exits 2 without a gate action. Update the checklist to require the persisted proven commit and remove the previous-remote-head instruction.

HIGHEST: P3
OVERALL: needs-attention
