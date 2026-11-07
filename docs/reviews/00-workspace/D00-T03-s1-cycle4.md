## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:27:56Z
- Candidate: 40ae7311c780dcf124fe2bc3333b12a0cf49d390
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 40ae7311c780dcf124fe2bc3333b12a0cf49d390
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: f609461cfda79aab

1. **P1, plan, line 73 (handoff to D00 T03 §6):** The completion consumer cannot recognise this section’s proven skips. §6 requires `ci_status.py --sha <pushed-head>` to exit 0, but the saved proven base is recovered only with `--gate`. A Markdown stamp can therefore receive `proceed` from the gate while the completion check returns `none` indefinitely. Specify a read-only check that consumes the same persisted proof, update the §6 command, and require an integration test showing a skipped build on the final stamp permits completion while an unproven skip refuses it.

2. **P2, plan, line 84:** “Clear it on any verdict” includes `pending`, `none` and `unavailable`, contradicting the cumulative two-hour limit. Clearing on these normal polling results restarts the budget on each invocation. Require clearing only on decisive success or red, retaining the original timestamp across all waiting verdicts. Test alternating `pending`, `none` and `unavailable` across separate processes until `unverified`, alongside the interrupted-first-lookup test.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: --use-proven is the read-only check D00 T03 §6 consumes (P1) in the D00 T03 §1 contract, this commit.
- Planned: the wait clears only on green or red, tested across processes (P2) in the D00 T03 §1 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:30:11Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 40ae7311c780dcf124fe2bc3333b12a0cf49d390
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 16238c12ae2b8401

## adversarial: needs-attention

1. **P2, `scripts/ci_status.py:329`: Valid fallback proof is discarded when the primary record is unreadable.** `peek_proven()` reads `ci-gate.json` before accepting `ci-gate.cleared`; a sharing violation sends it into the exception handler and loses the valid marker. Reproduce by holding the primary record with a Windows lock denying reads and replacement, letting a successful gate save its fallback marker, then checking a subsequent Markdown-only commit with `--sha <commit> --use-proven`. With no build run, this returns `none` instead of the proven skip and blocks completion. An in-memory reproduction confirmed that `peek_proven()` returns `None` while `read_gate()` recovers the same marker. Handle primary-record read failures independently so they do not discard valid fallback proof.

HIGHEST: P2
OVERALL: needs-attention
