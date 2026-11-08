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

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-08T03:07:12Z
- Candidate: 8dd6d22a56525991b22886ab8d5e650fc3aff946
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 8a629aaec3c6d3ded74b438212908f0be3e9be71
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 26
- Digest: 54ec574ee87d30b9

Part 1: completeness

The implementation appears functionally complete for its scoped workflow, but its regression coverage has one gap. I ran 17 read-only tests successfully. Full checkpoint and live-session tests were not run because they create files.

1. **present**: Automatic hook registration for Bash and PowerShell, including interpreter discovery. Evidence: `scripts/tests/test_hook_commands.py:36` and `:51` exercise the configured commands.
2. **present**: Detached and foreground reviews are refused with instructions for retrying through `run_in_background`; supported background launches pass. Evidence: `scripts/dispatch_hook.py:307`; `scripts/tests/test_dispatch_hook.py:36`.
3. **present**: Modified or staged reviewer pins block reviews and stamps; modified findings do not. Evidence: `scripts/dispatch_hook.py:283`; `scripts/tests/test_dispatch_hook.py:178` and `:189`.
4. **present**: Help, pin checks, unrelated commands and quoted mentions remain usable. Evidence: `scripts/tests/test_dispatch_hook.py:120`, `:130` and `:144`.
5. **present**: Malformed payloads and missing parser dependencies produce diagnostics and follow the explicitly specified fail-open policy. Evidence: `scripts/dispatch_hook.py:317`; `scripts/tests/test_dispatch_hook.py:215` and `:224`.
6. **present**: Agent-specific launch, wait and final-result instructions have coverage. Evidence: `scripts/tests/test_review_dispatch.py:1250`.
7. **present**: Real-session test fixtures include the parser dependency, check hook refusal, and assert approval, negative verdict and process failure delivery. Evidence: `scripts/tests/test_guard_e2e.py:137`, `:158` and `:182`. Execution remains unverified in this review.
8. **present**: Refusal requires no undo because the hook does not execute the rejected command or save state. Retrying reads the current pin again. Evidence: `scripts/dispatch_hook.py:292`. UI controls, shortcuts, dialogs and independent persistence are not applicable to this section.
9. **present**: The threat model distinguishes mistake prevention from authoritative enforcement and names the owning sections. Evidence: `scripts/dispatch_hook.py:14`; `scripts/tests/test_dispatch_hook.py:195`.
10. **missing-in-scope**: The dispatch parser’s new ANSI-C quoting behavior lacks its own regression test. `scripts/tests/test_dispatch_hook.py:115` covers ordinary escaping, but not `$'...'`. Removing `scripts/dispatch_hook.py:175` through `:179` in memory leaves all 17 read-only tests passing, yet incorrectly allows a detached review using `--prompt $'review\';notes.md' &`. Add both allowed and detached cases that fail when this change is reverted. The earlier plan review explicitly requires regression coverage for both parsers (`docs/reviews/00-workspace/D00-T01-s3-cycle2.md:21`).

Part 2: excellence

**Alternative:** The locally inspected Recto dispatch hook, supplemented by Resolute’s dirty-pin guard, is the strongest directly comparable alternative identified by the governing contract. Recto enforces background launch without importing a sibling parser; Resolute checks the pin before dispatch or stamping. Broad superiority is not established by this review.

1. **suggestion**: Dependency failure weakens operational recovery compared with Recto’s standalone hook. Bosun reports a missing parser but permits the review (`scripts/dispatch_hook.py:64`, `:325`), leaving diagnosis to the session. Target: an integrated startup health check identifies an unavailable parser before the first review, names the recovery action, and verifies recovery in one rerun. Preserve the decided fail-open policy.

Gap list

1. **missing-in-scope**: Add dispatch-hook regression tests for allowed and detached ANSI-C quoted prompt paths that fail when `scripts/dispatch_hook.py:175` is reverted.

COMPLETION: gaps

- Fixed in fbd42c6: `test_an_ansi_c_quoted_prompt_is_one_word` refuses a detached review whose prompt is ANSI-C quoted and passes the same review in the background; it fails with the dispatch hook's $'...' handling removed (gap 1).
