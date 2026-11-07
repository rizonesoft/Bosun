## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:24:55Z
- Candidate: fa3161e7497a959c524f36ab577d583cae35ceae
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: fa3161e7497a959c524f36ab577d583cae35ceae
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: 1053a7f11878e78c

1. **P1, plan, line 134 (Test checkpoint):** The checkpoint runs only `test_review_dispatch.py`, but fourth-repair refusal and the new exhaustion-to-stamp proof belong to `test_todo_graph.py`. It can pass with repair limits, parking or recovery broken. Include `test_todo_graph.py` and `test_run_guard.py`, and require the checkpoint to fail when an ungranted fourth repair succeeds or repairable work incorrectly ends the run.

2. **P2, plan, line 129 (invalid repair cap):** The section requires an invalid cap to remain `needs-repair` while making `repair` refuse, but defines no recovery route for the unattended consumer. The phase skill consequently selects work it cannot execute and can repeatedly retry the same refusal. Add an actionable configuration-error route that preserves review evidence, prevents repeated repair attempts and lets other runnable rows proceed. Test the invalid-pin transition and recovery after correcting the pin.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: the checkpoint runs all three suites (P1) in the D00 T03 §3 contract, this commit.
- Planned: a bad repair cap reads waiting-person, so a run passes over it and it recovers when corrected (P2) in the D00 T03 §3 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:30:18Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: fa3161e7497a959c524f36ab577d583cae35ceae
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 197ff4af2769edee

### adversarial: approve

No actionable adversarial defects identified in the candidate diff. Review was static; tests were not run under the read-only constraint.

HIGHEST: none
OVERALL: approve

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:32:02Z
- Candidate: 4d52e066683205105f591b5eb709fd6d609d581e
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: fa3161e7497a959c524f36ab577d583cae35ceae
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 28
- Digest: ce0589beda75cc1a

The feature is not complete: malformed or unreadable repair settings bypass the new refusal path. A read-only probe of the candidate reproduced both silent fallback to three cycles and an uncaught exception. No files were modified; the file-writing test suites were not run.

Part 1: completeness

1. **present:** CLI entry points, required reasons and help for `repair`, `park` and `unpark`: `scripts/todo-graph.py:90`. Menus, tooltips, shortcuts and dialogs do not apply to this `Surface: no` section.
2. **present:** A repair starts a separate findings cycle, preserves earlier findings and refuses duplicate consumption: `scripts/lib/todo_graph.py:1055`; tests `test_repair_starts_a_new_cycle_keeping_the_findings` and `test_running_repair_again_spends_nothing`.
3. **present:** Explicit numeric cap validation and recovery after correction: `scripts/lib/todo_graph.py:1018`; `test_a_bad_repair_cap_refuses_repair_but_still_answers`. Dispatcher read-back coverage: `scripts/tests/test_review_dispatch.py:1220`.
4. **missing-in-scope:** Malformed or unreadable configuration needs the same actionable refusal and `waiting-person` state. At `scripts/lib/todo_graph.py:1013`, truncated TOML and `PermissionError` silently select three cycles. Valid TOML containing `schedule = 3` raises `AttributeError`, which `section_disposition` does not catch at line 1109. Thus an ordinary configuration mistake can either spend a cycle under an unverified policy or interrupt disposition reporting for all rows. Add regression cases for these conditions; preserve findings and cycle counts.
5. **present:** Parking prevents repair and stamping, blocks dependants, and has an explicit operator reversal granting one additional cycle: `scripts/lib/todo_graph.py:1070`, `:1077`, `:1285`; tests at `scripts/tests/test_todo_graph.py:647`, `:669`, `:1539`.
6. **present:** Needs, dependencies and hosts precede repair eligibility; the guard accepts repairable work and skills route exhaustion to parking: `scripts/lib/todo_graph.py:1086`, `scripts/lib/run_guard.py:308`, `.codex/skills/process-phase/SKILL.md:39`; `test_a_run_with_only_repairable_work_left_goes_on`.
7. **present:** Persistence uses temporary-file replacement and fresh reads. Failed projection writes restore prior files; failed rollback names a recovery command: `scripts/lib/todo_graph.py:95`, `:1022`; tests at `scripts/tests/test_todo_graph.py:586`, `:595`, `:612`.
8. **present:** Refusals cover unresolved references, blank or multiline reasons, invalid date format, inappropriate lifecycle transitions and exhausted allowance: `scripts/lib/todo_graph.py:1046`; `test_repair_refuses_what_it_cannot_do` and the parking tests.
9. **present:** Review lineage and completion remain mandatory after repair. Exhaustion, grant, repair and successful stamp have an integrated test: `scripts/tests/test_todo_graph.py:1539`. Its helper rejects earlier-cycle evidence and verifies fresh build-start ordering at lines 1562 onward.

Part 2: excellence

Comparison: **Temporal with bounded retries and its human-assisted resumable workflow pattern**, a strong alternative for durable retry orchestration, although adopting it requires infrastructure and integration. Its documented patterns distinguish retryable failures from validation failures and support parking for human correction. [Temporal error-handling patterns](https://github.com/temporalio/documentation/blob/main/docs/design-patterns/error-handling-patterns.mdx).

1. **in-scope:** Configuration failure handling falls short of that explicit recovery model. The candidate can silently substitute policy or crash instead of identifying an operator-fixable condition. **Target:** every malformed, wrongly typed or unreadable repair configuration produces an actionable `waiting-person` reason; `repair` refuses without writes; disposition still reports unrelated rows; correcting configuration restores eligibility without consuming a cycle. Extend `scripts/tests/test_todo_graph.py:555` accordingly.

No comparative speed or step-count advantage was established. GUI accessibility and visual polish are outside this section’s designed surface.

Gap list

1. **missing-in-scope:** Handle malformed, wrongly typed and unreadable repair configuration consistently, without fallback spending or failure of the whole disposition query; add regression coverage.
2. **in-scope:** Meet the explicit recovery target: actionable configuration diagnostics, zero consumed cycles, continued reporting of other rows and automatic eligibility restoration after correction.

COMPLETION: gaps
