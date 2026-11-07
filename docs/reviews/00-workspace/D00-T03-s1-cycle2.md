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

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:12:47Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 51729b1bfc594bf6f6f8b0ffca95f46c08961726
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 8
- Digest: 1f9be358024e348f

### adversarial: needs-attention

1. **P2, `scripts/ci_status.py:428`: The durable wait starts after polling finishes.** With unavailable CI and the prescribed `--wait-minutes 30`, repeated invocations return `wait` after 120 minutes and only return `unverified` after 150 minutes. Reproduced with an injected clock. Cancellation before the first polling window finishes persists no waiting timestamp, so restarting loses that elapsed time entirely. Persist the start before polling and cap each invocation by the remaining durable budget.

2. **P2, `scripts/ci_status.py:288-294`: An undeletable clearing marker repeatedly erases new repair attempts.** After the fallback marker is created, a Windows handle denying deletion can make `marker.unlink()` fail while the state file remains writable. Every subsequent read then replaces the current repair record with the old marker’s `proven` value. Reproduced by simulating that sharing violation: five distinct red commits all returned `repair, 1`, never `exhausted`. Record that the marker has been consumed durably, so deletion failure cannot reset subsequent attempts.

HIGHEST: P2
OVERALL: needs-attention

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:14:11Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 51729b1bfc594bf6f6f8b0ffca95f46c08961726
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: b7a765af72ce96a9

### consistency: needs-attention

1. **P3, `scripts/ci_status.py:399`: CLI help omits the new `unverified` action.** The module documentation and process-phase policy now define five actions, but `--gate` help still lists only four. Reproduce with `python scripts/ci_status.py --help`. Add `unverified` to the option help so the public interface matches its implementation and consumer policy.

HIGHEST: P3
OVERALL: needs-attention

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:16:17Z
- Candidate: c35e74a4b1cb221c85696964f4fe0cb8827852b2
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 51729b1bfc594bf6f6f8b0ffca95f46c08961726
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 45
- Digest: 2ad247825fe350c4

The feature is not complete, and it does not yet beat GitHub CLI across reliability and responsiveness. Reviewed `c35e74a` and the supplied range. No files were changed. Verification used source inspection and read-only, in-memory probes; the file-writing test suite was not run.

**Part 1: completeness**

This CLI tells an unattended campaign whether a pushed commit permits proceeding, waiting or repairing. GUI controls, tooltips, shortcuts, dialogs and visual accessibility are outside its declared `Surface: no`.

1. **present:** CLI entry point, options, defaults and usage refusals. Evidence: `scripts/ci_status.py:389`; tests `test_needs_sha`, `test_bad_sha_is_usage` and `test_wait_minutes_needs_gate`.

2. **present:** Exact-commit lookup, workflow verdicts, cancellation handling and red-first exit precedence. Evidence: `scripts/ci_status.py:209` and `:245`; tests in `Verdicts`, `Commit` and `OverallExit`.

3. **missing-in-scope:** The remembered base can falsely prove a skip. At `scripts/ci_status.py:425`, the last proven commit substitutes for the actual pre-push head. Suppose A passed, B changed code but remained unverified, and C reverts B’s code plus edits Markdown. `A..C` contains only Markdown, while the actual push `B..C` changes code. With no build run yet, `skip_reason` can declare it skipped. Persist the actual push boundary separately from the last proven commit and test this sequence. Evidence: `scripts/ci_status.py:191`, `:223` and `:425`.

4. **present:** Repair counting, repeat-check idempotence and normal green reset. Evidence: `scripts/ci_status.py:328`; tests `test_repairs_count_by_commit_then_exhaust` and `test_count_survives_a_new_process_and_green_clears_it`.

5. **present:** Main state uses temporary-file replacement, read-back and explicit handling of write failures. Evidence: `scripts/ci_status.py:270` and `:299`; tests for corrupt records, locked records, undeletable markers and unavailable storage.

6. **missing-in-scope:** The fallback clearing marker bypasses atomic persistence. `scripts/ci_status.py:336` writes it directly, while `:288` accepts any nonempty content as a completed reset. An interrupted or disk-full write can leave a partial marker that resets attempts on restart. Write the marker through temporary-file replacement, validate its complete record, and test interrupted writes.

7. **missing-in-scope:** Waiting is not bounded end to end. The first report runs before the durable deadline is applied; Git calls retain independent 120-second limits; expired HTTP calls still receive at least one second. In-memory timeout probes reproduced **7 seconds for a six-second budget**, and **1,801 additional seconds after the durable two-hour limit had already expired**. Apply the shared deadline before the first tool call and launch no further calls after expiry. Evidence: `scripts/ci_status.py:391`, `:428` and `:435`.

8. **present:** Text/JSON output separation, ordinary waiting feedback and action consumption. Evidence: `scripts/ci_status.py:442`, `:451`, `.codex/skills/process-phase/SKILL.md:55`; tests `test_waiting_prints_progress_and_keeps_stdout_one_object` and `test_three_mirrors_hold_the_same_policy_naming_every_action`.

9. **missing-in-scope:** Required restart evidence is incomplete. `test_waiting_on_one_commit_is_limited_across_invocations` and `test_a_restart_after_a_push_still_has_its_base` call the same process, despite the contract requiring separate processes. The timeout test checks shrinking arguments without asserting total elapsed time. Add actual cross-process wait/base tests and elapsed-time assertions covering item 7. Evidence: `scripts/tests/test_ci_status.py:443`, `:503` and `:536`.

**Part 2: excellence**

The strongest direct alternative is **GitHub CLI**, specifically `gh run watch --exit-status` plus `gh run view`. It displays step progress, refreshes every three seconds by default and exposes failed-step logs. This comparison concerns documented behaviour, not a measured network benchmark. [Watch documentation](https://cli.github.com/manual/gh_run_watch), [view documentation](https://cli.github.com/manual/gh_run_view).

1. **in-scope:** Reliability does not yet justify replacing direct run evidence. The false-skip path in Part 1 item 3 undermines the gate’s central advantage. **Target:** zero `proceed` results for the A/B/C scenario without a successful run or proof against the actual push boundary.

2. **in-scope:** Recovery timing violates the feature’s stronger bounded-wait promise. **Target:** tool execution and sleeps share the remaining budget; an expired durable budget launches zero network calls. Same defect as Part 1 item 7.

3. **out-of-scope:** Result detection is slower by design: 60-second polling versus GitHub CLI’s three-second default. A completion immediately after polling can cost nearly another minute. **Target:** an optional attended mode with detection within three seconds plus request latency. This needs a design decision and a new `00-workspace` section because §1 explicitly specifies 60-second polling.

**Consolidated gaps**

1. **missing-in-scope:** Persist the actual push boundary; prevent false skips after unverified code changes and reverts.
2. **missing-in-scope:** Make clearing-marker persistence atomic and reject incomplete records.
3. **missing-in-scope:** Enforce invocation and durable deadlines across every tool call and sleep.
4. **missing-in-scope:** Add required separate-process recovery tests and total elapsed-time assertions.
5. **in-scope:** Demonstrate trustworthy skip decisions against actual push evidence.
6. **in-scope:** Meet the bounded-wait guarantee under slow or unavailable tools.
7. **out-of-scope:** Design optional faster attended polling in `00-workspace`; no section was filed because this review forbids file changes.

COMPLETION: gaps
