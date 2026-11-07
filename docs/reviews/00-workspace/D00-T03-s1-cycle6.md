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

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:08:12Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 956ba7961e611f2f

### adversarial: needs-attention

1. **P2, `scripts/ci_status.py:511`: An expired wait resets when `--sha` is a branch name.** Reproduce by repeatedly running `python scripts/ci_status.py --gate --sha main` while CI remains unavailable for two hours. The recorded wait uses the resolved SHA, but the expired deadline prevents resolving `main`. The exception handler at line 520 replaces that record with a fresh wait keyed by `"main"`. The next invocation resolves the SHA and starts another fresh wait. This can repeat indefinitely instead of returning `unverified`. A read-only, in-memory reproduction confirmed both timestamp resets and renewed tool calls. Preserve the existing wait when name resolution exhausts its deadline.

HIGHEST: P2
OVERALL: needs-attention
