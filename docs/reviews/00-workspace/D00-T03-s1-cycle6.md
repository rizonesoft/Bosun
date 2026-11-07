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

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:09:30Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: c84ab3eb2f221160

### consistency: needs-attention

1. **P2, `scripts/ci_status.py:511`: A new full SHA inherits an older commit’s expired wait.** After commit A remains unverified for two hours, push commit B and run `python scripts/ci_status.py --gate --sha <B-full-SHA>`. The expired deadline prevents resolving B, and the exception handler retains A’s wait. Every retry returns `unverified` without checking B, even if B’s CI is green. This contradicts the per-commit waiting policy and differs from `--sha HEAD`, which can start B’s fresh budget. A read-only, mocked reproduction confirmed zero tool calls and A passed to `gate_action`. Ensure a new explicit SHA can establish its own wait budget.

HIGHEST: P2
OVERALL: needs-attention

## Round 3: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:10:38Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: quality
- Lines: 6
- Digest: 48997ba68d554130

### quality: needs-attention

1. **P3, `scripts/tests/test_ci_status.py:946`: The expired-wait test accepts the wrong action.** The fixture reaches the two-hour limit with an unreadable HEAD, but accepts both `wait` and `unverified`. Returning `wait` indefinitely would therefore pass this assertion. Reproduce by substituting `wait` for the resulting action: the assertion still succeeds. Require `unverified` and assert that the recorded wait remains unchanged.

HIGHEST: P3
OVERALL: needs-attention
