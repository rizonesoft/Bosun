## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:06:03Z
- Candidate: 4122795f40916e899f7b12d3c286599f0e6458da
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 5
- Digest: 7c10db08a9229a47

1. **P2, plan, line 90:** The new direct `.git` reader lacks a defined failure path and complete acceptance coverage. Comparing one real repository with `git rev-parse HEAD` can pass while detached HEAD, packed refs or linked worktrees remain broken. Require separate fixtures for all four promised layouts, plus an unreadable or missing HEAD/ref. Specify that read failures produce a diagnostic and non-success gate result without a traceback or an out-of-budget subprocess, and test those outcomes.

HIGHEST: P2
PLAN: gaps
OVERALL: needs-attention

- Planned: all four layouts and an unreadable HEAD are tested, with a gate answer and no tool past the limit (P2) in the D00 T03 §1 contract and its tests.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:08:12Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 956ba7961e611f2f

### adversarial: needs-attention

1. **P2, `scripts/ci_status.py:511`: An expired wait resets when `--sha` is a branch name.** Reproduce by repeatedly running `python scripts/ci_status.py --gate --sha main` while CI remains unavailable for two hours. The recorded wait uses the resolved SHA, but the expired deadline prevents resolving `main`. The exception handler at line 520 replaces that record with a fresh wait keyed by `"main"`. The next invocation resolves the SHA and starts another fresh wait. This can repeat indefinitely instead of returning `unverified`. A read-only, in-memory reproduction confirmed both timestamp resets and renewed tool calls. Preserve the existing wait when name resolution exhausts its deadline.

HIGHEST: P2
OVERALL: needs-attention

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:09:30Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: c84ab3eb2f221160

### consistency: needs-attention

1. **P2, `scripts/ci_status.py:511`: A new full SHA inherits an older commit’s expired wait.** After commit A remains unverified for two hours, push commit B and run `python scripts/ci_status.py --gate --sha <B-full-SHA>`. The expired deadline prevents resolving B, and the exception handler retains A’s wait. Every retry returns `unverified` without checking B, even if B’s CI is green. This contradicts the per-commit waiting policy and differs from `--sha HEAD`, which can start B’s fresh budget. A read-only, mocked reproduction confirmed zero tool calls and A passed to `gate_action`. Ensure a new explicit SHA can establish its own wait budget.

HIGHEST: P2
OVERALL: needs-attention

## Round 3: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:10:38Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: quality
- Lines: 6
- Digest: 48997ba68d554130

### quality: needs-attention

1. **P3, `scripts/tests/test_ci_status.py:946`: The expired-wait test accepts the wrong action.** The fixture reaches the two-hour limit with an unreadable HEAD, but accepts both `wait` and `unverified`. Returning `wait` indefinitely would therefore pass this assertion. Reproduce by substituting `wait` for the resulting action: the assertion still succeeds. Require `unverified` and assert that the recorded wait remains unchanged.

HIGHEST: P3
OVERALL: needs-attention

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:12:42Z
- Candidate: 541c09c29aceccd649deb6994a7f2d92ea3511e0
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4122795f40916e899f7b12d3c286599f0e6458da
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 33
- Digest: d4be059dc5537043

The feature is not complete for all accepted commit references. It also falls short of the strongest CLI alternative in recovery and progress detail.

Reviewed the specified range at `541c09c`; the working tree was clean. No files were modified. Evidence below combines source inspection with an in-memory reproduction; the filesystem-writing test suite was not run.

**Part 1: completeness**

This feature lets the unattended campaign determine whether a pushed commit is proven and choose its next action. Its consumer is `process-phase`. GUI controls, tooltips, shortcuts, dialogs and visual accessibility are not applicable to this section’s `Surface: no` contract.

1. **present:** CLI commands, help and argument refusal paths. `scripts/ci_status.py:449` defines the options and rejects incompatible combinations. Tests include `test_needs_sha`, `test_the_gate_takes_no_workflow_filter` and `test_use_proven_refuses_other_modes`.
2. **present:** Distinct empty, pending, failed, unavailable and proven results, with red-first aggregation. Evidence: `scripts/ci_status.py:217`, `:255`; tests at `scripts/tests/test_ci_status.py:113`, `:128`, `:321`, `:337`.
3. **present:** Exact-commit lookup and protection against an older commit’s green result. Evidence: `scripts/ci_status.py:218`; tests at `scripts/tests/test_ci_status.py:157`, `:166`, `:180`.
4. **present:** Skip evidence from workflow filters and the complete diff against a proven base, including exact reverts and failed intervening commits. Evidence: `scripts/ci_status.py:182`; tests at `scripts/tests/test_ci_status.py:239`, `:743`, `:753`, `:760`.
5. **present:** Durable repair counting, reset after green and recovery from corrupt, locked or unwritable state. State uses temporary-file replacement. Evidence: `scripts/ci_status.py:280`, `:315`, `:368`; tests at `scripts/tests/test_ci_status.py:446`, `:471`, `:477`, `:484`, `:511`.
6. **present:** Persistent wait start, bounded subprocesses and sleeps, and interruption recovery. Evidence: `scripts/ci_status.py:354`, `:487`, `:530`; tests at `scripts/tests/test_ci_status.py:527`, `:545`, `:820`, `:826`, `:1009`.
7. **present:** Direct `HEAD` reading for ordinary branches, detached heads, packed refs and linked worktrees, plus unreadable-HEAD refusal. Evidence: `scripts/ci_status.py:419`; tests at `scripts/tests/test_ci_status.py:895`, `:903`, `:934`.
8. **missing-in-scope:** A new commit addressed by branch name cannot recover from the previous commit’s expired wait. At `scripts/ci_status.py:509`, the old deadline prevents resolving `--sha main`; the exception path retains the old wait indefinitely. An in-memory reproduction returned `unverified` with zero tool calls twice for `main`, while the new full SHA immediately returned `proceed`. Add coverage for A expiring, `main` advancing to B, and B receiving its own budget. Preserve the prohibition on subprocesses beyond A’s deadline.
9. **present:** Text and JSON output, wait feedback, failure links and explicit reasons for `unverified`. Evidence: `scripts/ci_status.py:400`, `:558`; tests at `scripts/tests/test_ci_status.py:138`, `:611`, `:809`, `:1057`.
10. **present:** Read-only proof checks and campaign action consumption. Evidence: `scripts/ci_status.py:330`, `.codex/skills/process-phase/SKILL.md:56`; tests at `scripts/tests/test_ci_status.py:554`, `:1132`. There is no user-content mutation requiring an undo command; green resets campaign bookkeeping.

**Part 2: excellence**

The strongest direct alternative is GitHub CLI: `gh run list --commit <SHA>` followed by `gh run watch <run-id> --exit-status`. It supports commit filtering, step-level progress and a default three-second refresh. It does not itself supply Bosun’s durable campaign repair policy. [Run listing](https://cli.github.com/manual/gh_run_list), [Run watching](https://cli.github.com/manual/gh_run_watch).

1. **in-scope:** Recovery requires changing the command’s commit reference after the branch-name failure above. The alternative can select the new commit’s runs without inheriting Bosun’s expired local wait. **Target:** one invocation of the existing branch-name command checks B after A expires, with no state deletion or reference substitution.
2. **out-of-scope:** Waiting feedback reports only workflow verdicts, whereas `gh run watch` shows individual steps. An operator cannot distinguish a progressing build from a stalled job through this command alone (`scripts/ci_status.py:560`). **Target:** an optional diagnostic mode reports the active job/step and its run link on each existing poll, retaining one JSON object on stdout. Owner: `00-workspace`, new section for richer CI diagnostics. Keep the agreed 60-second campaign polling policy.

**Gap list**

1. **missing-in-scope:** Resolve a newly advanced branch after its previous commit’s wait expires, with a regression test preserving deadline guarantees.
2. **in-scope:** Restore one-command recovery for branch-name callers without manual state deletion or SHA substitution.
3. **out-of-scope:** Add optional job/step progress diagnostics in a new `00-workspace` section; proposed here only, because this review must not modify files.

COMPLETION: gaps
