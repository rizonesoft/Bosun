## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:33:44Z
- Candidate: 43e733660d586a3aea0472eb1c3ae93fa1029d1f
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 43e733660d586a3aea0472eb1c3ae93fa1029d1f
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 5
- Digest: c0f3e6cc988af5d5

1. **P2, plan, line 127:** Require `review_dead_end` to use the cycle’s initiating committed review policy when detecting exhausted rounds. At this commit, it uses the current pin through `round_cap`, while the dispatcher preserves the initiating policy. Changing `max_low_rounds` during an unfinished cycle can therefore leave the guard offering work the dispatcher refuses, or trigger repair prematurely. Add tests changing the pin in both directions after a cycle starts and proving disposition and dispatcher agree.

HIGHEST: P2
PLAN: gaps
OVERALL: needs-attention

- Planned: exhausted rounds judged by the initiating policy (P2) in the D00 T03 §3 contract, this commit.
