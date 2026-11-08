## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-08T02:57:37Z
- Candidate: 8a629aaec3c6d3ded74b438212908f0be3e9be71
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 8a629aaec3c6d3ded74b438212908f0be3e9be71
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 11
- Digest: 7b44d75074bc2dd9

1. **P1, plan, line 123:** The checked-off end-to-end proof can run with a broken hook. `BackgroundReview.session()` copies only `review-dispatch.py` and `dispatch_hook.py`, but the hook imports sibling `tool-guard.py` at startup. Require copying that dependency and asserting that the installed hook actually refuses a detached launch before accepting a background launch. Fail the test on hook startup errors.

2. **P1, plan, line 123:** The test does not prove verdict delivery: it asserts only `EXIT` and `REVIEWER`, and its fake reviewer supplies only approval or process failure. Require assertions on the actual verdict, including a successful reviewer returning `needs-attention`, so losing or misreporting a negative review fails the checkpoint.

3. **P2, plan, lines 125 and 128:** The section changes both shell parsers, but its checkpoint omits `scripts/tests/test_tool_guard.py`. Require regression cases for both parsers, including an allowed quoted argument, and run both suites. Otherwise the stated checkpoint can pass with the force-push parser fix absent or broken.

4. **P2, plan, lines 105 and 124:** The proposed “hard guarantee” relies on remote branch protection without requiring verification or naming a section that establishes it. A test finding that statement in docstrings proves nothing about enforcement. Require read-back of applicable protection and bypass settings, or qualify the statement as conditional and assign verification to a named section.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: the real-session test copies tool-guard.py and proves the installed hook refuses first; a hook without its parser says so (P1) in the D00 T01 §3 contract, this commit.
- Planned: the real-session test asserts the verdict, approve, needs-attention and a failure (P1) in the D00 T01 §3 contract, this commit.
- Planned: the checkpoint runs the deny list's suite too (P2) in the D00 T01 §3 contract, this commit.
- Filed: D00 T03 §17 (branch protection on main, the hard guarantee against a force push, turned on and read back once the owner agrees) (P2)

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-08T03:04:52Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 8a629aaec3c6d3ded74b438212908f0be3e9be71
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 119cf991caf03b6d

### adversarial: approve

No actionable findings in the candidate range. Six read-only parser probes passed. Live-session tests were not run because they create files and launch Claude.

HIGHEST: none
OVERALL: approve
