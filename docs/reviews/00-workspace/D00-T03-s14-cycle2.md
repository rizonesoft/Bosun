## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:45:49Z
- Candidate: 4c9569dcc3ad97697fdd27e1721dc5c548416a8d
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4c9569dcc3ad97697fdd27e1721dc5c548416a8d
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: e9029ec73854348e

1. **P1, plan, line 458:** The end-to-end filed-stamp test explicitly uses the older policy, while the current-policy test calls `schedule_ok` with mocked policy lookup. Both can pass while the actual current-policy `stamp` path refuses filing. Add a committed current-policy fixture with two repairs, clean third-cycle rounds and completion gaps; invoke `stamp`, then validate in a fresh process. Assert the filed targets survive in the receipt and remain open. Exercise cycle-two refusal through the same path.

2. **P2, plan, line 456:** Runner routing remains contradictory. `process-phase/SKILL.md:52` instructs the per-row loop to handle `ready` and `needs-repair`, then “skip the rest,” which includes `needs-filing`; `process-plan/SKILL.md:24` still routes completion gaps unconditionally to repair or park. The existing tests merely check that `needs-filing` appears somewhere. Require both instructions and all mirrors to route third-cycle completion gaps to filing and stamping, and strengthen verification to reject these conflicting routes.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: a real current-policy filed stamp, and the cycle-two refusal, through stamp itself (P1) in the D00 T03 §14 contract, this commit.
- Planned: both runner skills route third-cycle gaps to filing in every mirror, with the old routes rejected by a test (P2) in the D00 T03 §14 contract, this commit.
