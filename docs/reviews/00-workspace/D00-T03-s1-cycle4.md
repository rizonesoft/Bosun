## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:27:56Z
- Candidate: 40ae7311c780dcf124fe2bc3333b12a0cf49d390
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 40ae7311c780dcf124fe2bc3333b12a0cf49d390
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: f609461cfda79aab

1. **P1, plan, line 73 (handoff to D00 T03 §6):** The completion consumer cannot recognise this section’s proven skips. §6 requires `ci_status.py --sha <pushed-head>` to exit 0, but the saved proven base is recovered only with `--gate`. A Markdown stamp can therefore receive `proceed` from the gate while the completion check returns `none` indefinitely. Specify a read-only check that consumes the same persisted proof, update the §6 command, and require an integration test showing a skipped build on the final stamp permits completion while an unproven skip refuses it.

2. **P2, plan, line 84:** “Clear it on any verdict” includes `pending`, `none` and `unavailable`, contradicting the cumulative two-hour limit. Clearing on these normal polling results restarts the budget on each invocation. Require clearing only on decisive success or red, retaining the original timestamp across all waiting verdicts. Test alternating `pending`, `none` and `unavailable` across separate processes until `unverified`, alongside the interrupted-first-lookup test.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: --use-proven is the read-only check D00 T03 §6 consumes (P1) in the D00 T03 §1 contract, this commit.
- Planned: the wait clears only on green or red, tested across processes (P2) in the D00 T03 §1 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:30:11Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 40ae7311c780dcf124fe2bc3333b12a0cf49d390
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 16238c12ae2b8401

## adversarial: needs-attention

1. **P2, `scripts/ci_status.py:329`: Valid fallback proof is discarded when the primary record is unreadable.** `peek_proven()` reads `ci-gate.json` before accepting `ci-gate.cleared`; a sharing violation sends it into the exception handler and loses the valid marker. Reproduce by holding the primary record with a Windows lock denying reads and replacement, letting a successful gate save its fallback marker, then checking a subsequent Markdown-only commit with `--sha <commit> --use-proven`. With no build run, this returns `none` instead of the proven skip and blocks completion. An in-memory reproduction confirmed that `peek_proven()` returns `None` while `read_gate()` recovers the same marker. Handle primary-record read failures independently so they do not discard valid fallback proof.

HIGHEST: P2
OVERALL: needs-attention

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:31:36Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 40ae7311c780dcf124fe2bc3333b12a0cf49d390
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: f2ba3a3caaa95190

## consistency: approve

No actionable consistency findings in the candidate range.

HIGHEST: none
OVERALL: approve

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:33:40Z
- Candidate: c6d8e969fc6146c2e9d9e1474e3c435c956b7004
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 40ae7311c780dcf124fe2bc3333b12a0cf49d390
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 37
- Digest: 748e57299b2855e2

The feature is **not complete**, and it does **not yet beat GitHub CLI on feedback speed**. Two completeness gaps reproduced against the candidate code using in-memory fixtures. No files were modified; the full suite was not run.

**Part 1: completeness**

This feature lets the unattended campaign determine whether a specific commit is proven and choose its next action. UI controls, shortcuts, dialogs, visual accessibility and user-facing undo do not apply to this section’s `Surface: no` contract.

1. **present:** CLI entry points, help and invalid-option refusals. `scripts/ci_status.py:419` defines the arguments; line 438 rejects partial workflow checks with `--gate`. Tests include `test_the_gate_takes_no_workflow_filter` and `test_use_proven_refuses_other_modes`.

2. **present:** Exact-commit lookup, pending/empty/unavailable states and red-first exit precedence. `scripts/ci_status.py:211` and `:247`; tests include `test_green_older_commit_cannot_satisfy_a_newer_one`, `test_newer_attempts_of_the_same_commit_decide` and `test_table`.

3. **present:** Whole-range path-filter checks, conservative refusal when evidence cannot be read, and actual runs taking precedence over inferred skips. `scripts/ci_status.py:179`; tests include `test_code_commit_before_a_markdown_stamp_is_not_skipped`, `test_unprovable_cases_read_none` and `test_a_run_outranks_a_skip`.

4. **missing-in-scope:** A complete revert to the proven tree cannot obtain the promised skip. `scripts/ci_status.py:197` rejects an empty diff even after successful ancestry and diff checks. Reproduction returned no skip for identical trees. The contract at `todo/00-workspace/TODO-03-campaign-readiness.md:79` explicitly permits fully reverting the failed change; `test_a_full_revert_really_is_unchanged_inputs` additionally changes Markdown, leaving the exact-revert case uncovered. Require a distinct descendant commit with identical inputs to pass when no actual run overrides that proof.

5. **present:** Durable repair accounting, automatic reset after success, atomic replacement and fallback recovery for unavailable storage. `scripts/ci_status.py:272`, `:309` and `:360`; tests cover corruption, failed writes, locked records, incomplete markers and separate-process recovery.

6. **present:** Read-only consumption of saved proof, including the fallback marker. `scripts/ci_status.py:325`; tests include `test_use_proven_recognises_a_skipped_final_stamp_and_changes_nothing` and `test_use_proven_finds_the_marker_when_the_record_is_locked`.

7. **present:** Persisted waiting across interrupted CI lookups and restarts, with progress on stderr and one JSON result on stdout. Tests include `test_the_start_of_a_wait_stands_if_the_first_lookup_is_interrupted`, `test_alternating_waits_keep_the_first_start_across_processes` and `test_waiting_prints_progress_and_keeps_stdout_one_object`.

8. **missing-in-scope:** The cumulative two-hour deadline does not bound tool calls when `--wait-minutes` is omitted. `scripts/ci_status.py:459` returns the normal 120-second timeout before consulting the deadline. With one second remaining, an in-memory reproduction launched two 120-second `gh` calls, including one after expiry, and finished **239 seconds late**. Require every gate invocation to respect the persisted deadline, with regression coverage for the default invocation.

9. **present:** Actionable refusal reasons and campaign consumption. `scripts/ci_status.py:360` supplies `unverified` reasons; `.codex/skills/process-phase/SKILL.md:56` consumes all actions. `test_every_unverified_says_why_and_the_action_stays_last` and `test_three_mirrors_hold_the_same_policy_naming_every_action` cover these contracts.

**Part 2: excellence**

The strongest directly applicable alternative is **GitHub CLI**, using `gh run list --commit` to identify runs and `gh run watch <run-id> --exit-status` to monitor them. Its documented watcher displays step progress and refreshes every three seconds by default. This comparison concerns monitoring, not an equivalent persisted campaign repair policy. [GitHub CLI documentation](https://cli.github.com/manual/gh_run_watch)

1. **out-of-scope:** Completion feedback can lag almost 60 seconds, versus approximately three seconds plus request latency with `gh run watch`. Evidence: `scripts/ci_status.py:60` and `:508`. Target: detect a changed verdict within three seconds plus request latency, while retaining bounded waits and restart recovery. This requires a design decision because §1 explicitly mandates 60-second polling; assign it to `00-workspace`.

**Consolidated gaps**

1. **missing-in-scope:** Recognise an exact revert to the proven tree; add coverage without an accompanying Markdown change.
2. **missing-in-scope:** Enforce the persisted deadline on default gate invocations; launch no tool after expiry.
3. **out-of-scope:** Improve verdict detection from 60-second polling toward three seconds plus request latency, through a `00-workspace` design change.

COMPLETION: gaps
