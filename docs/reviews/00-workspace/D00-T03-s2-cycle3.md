## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:02:25Z
- Candidate: e598a1b4cb4a1be32bdff02262d3ec68ddc49351
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: e598a1b4cb4a1be32bdff02262d3ec68ddc49351
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: 13037728835620c6

1. **P1, plan, lines 97 and 99:** The skill acceptance checks do not prove that section execution follows the shared disposition. At the candidate, `process-phase` Step 3 still selects rows using `resolve` exit 0, which intentionally permits host-blocked rows; `process-plan` also still says completion-gap sections are not runnable. The specified text checks can pass with these contradictory instructions present. Require every execution decision to re-read disposition, remove conflicting routes in all mirrors, and test that a host-blocked row is skipped while a repairable row is routed to repair.

2. **P2, plan, lines 94 and 105:** The desktop tests can pass with broken native probes. Existing locked/disconnected tests replace `session_active` and `input_desktop` with lambdas, while the real-probe test only checks that the result is Boolean. Require tests exercising the native-call wrappers with simulated WTS and desktop responses, including failure, disconnection and a locked desktop. Otherwise an always-active or always-Default wrapper can pass the checkpoint and incorrectly admit desktop work.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: execution re-reads disposition, the conflicting routes go, in all mirrors (P1) in the D00 T03 §2 contract, this commit.
- Planned: the native desktop probes are tested through simulated Windows calls (P2) in the D00 T03 §2 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:05:53Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: e598a1b4cb4a1be32bdff02262d3ec68ddc49351
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: fee3bda462adaea5

## adversarial: approve

No actionable adversarial defects found in the candidate range. Review was read-only; tests were not run.

HIGHEST: none
OVERALL: approve
