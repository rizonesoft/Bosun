## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:09:03Z
- Candidate: 51729b1bfc594bf6f6f8b0ffca95f46c08961726
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 51729b1bfc594bf6f6f8b0ffca95f46c08961726
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 9
- Digest: fc7ff34092bbc6ce

1. **P1, plan, lines 72-73:** The wait is bounded per invocation, but the consumer repeats it indefinitely. Missing runs, lost `gh` authentication or an unwritable repair record can permanently block every later section. Add a durable campaign-level retry limit and an explicit recovery or parked-state handoff that preserves the unproven verdict. Test repeated invocations and session restarts, not just one polling window.

2. **P1, plan, line 73:** The previous remote head is required for skip proof, but no step persists it before pushing or restores the pending gate after a crash. A restart after a Markdown-only push can lose the base and wait forever for a legitimately skipped workflow. Persist the base, pushed SHA and outstanding gate together; test recovery after push and before gate completion. The mirror-text test cannot prove this handoff.

3. **P2, plan, line 74:** The durable-reset acceptance test does not require a new process between the locked-file green and the next red. An in-memory reset could pass while a restarted campaign inherits the exhausted budget. Require separate processes, read-back of the persisted clearing marker, and a defined non-successful reset outcome when neither deletion nor marker persistence succeeds.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: a durable two-hour limit on waiting, then unverified, which carries on (P1) in the D00 T03 §1 contract, this commit.
- Planned: the last proven commit is the base, so a restart keeps it (P1) in the D00 T03 §1 contract, this commit.
- Planned: separate-process reset test, with a defined outcome when nothing can be written (P2) in the D00 T03 §1 contract, this commit.
