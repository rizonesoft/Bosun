## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:06:03Z
- Candidate: 4122795f40916e899f7b12d3c286599f0e6458da
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 5
- Digest: 7c10db08a9229a47

1. **P2, plan, line 90:** The new direct `.git` reader lacks a defined failure path and complete acceptance coverage. Comparing one real repository with `git rev-parse HEAD` can pass while detached HEAD, packed refs or linked worktrees remain broken. Require separate fixtures for all four promised layouts, plus an unreadable or missing HEAD/ref. Specify that read failures produce a diagnostic and non-success gate result without a traceback or an out-of-budget subprocess, and test those outcomes.

HIGHEST: P2
PLAN: gaps
OVERALL: needs-attention

- Planned: all four layouts and an unreadable HEAD are tested, with a gate answer and no tool past the limit (P2) in the D00 T03 §1 contract and its tests.
