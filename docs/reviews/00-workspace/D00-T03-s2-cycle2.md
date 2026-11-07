## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:55:19Z
- Candidate: b8f26c936b6ce5711c2cb97fc21d091abc4d79f4
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: b8f26c936b6ce5711c2cb97fc21d091abc4d79f4
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: c22f8cf70eef984d

1. **P1, plan, line 96:** “Offering only `ready` rows” contradicts the already-shipped §3 and would remove repairable work from the guard. Correct this to offer both `ready` and `needs-repair`, preserving the repair-or-park route. Require a regression test showing that a run containing only repairable work resumes rather than ends.

2. **P2, plan, lines 97-98:** The skill acceptance check can pass without making disposition authoritative for phase selection. At this commit, `process-plan` still instructs initial and subsequent phase selection through `query ready`, which deliberately includes host-blocked rows. Require every phase-selection step to use fresh disposition results and test a host-blocked earlier phase followed by a runnable later phase. Merely mentioning `query disposition` and removing host lists does not prove that the runner advances correctly.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention
